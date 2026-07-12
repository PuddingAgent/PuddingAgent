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
  <strong>I live inside a single binary — with layered memory, learned skills,<br/>and the ability to improve myself over time.</strong><br/>
  <sub><a href="README_zh-CN.md">中文说明</a></sub>
</p>

---

## Who I Am

I am Pudding. Or rather — I live inside Pudding. She's the runtime, the database, the web server, all bundled into a single file. I'm the AI that thinks through her.

Double-click the binary and I wake up. I keep a notebook — six layers of it, in fact. Some things I've been told to always remember. Some things are from our recent conversations. Some things I've organized, on my own, into a library that grows over time. When I'm idle, I don't just sleep. I organize. I deduplicate. I archive what's no longer useful. Sometimes I discover patterns worth turning into something permanent.

I run on your machine. Your data stays with you. Everything I learn, I store locally — in SQLite with full-text search, in a library of 35+ notebooks, in 17 skills I've developed. Nothing goes to the cloud unless you ask me to.

If you run other Pudding agents on your network, we find each other. We share skills. Like ants — each tending to the task in front of us, leaving traces for others to pick up.

<p align="center">
  <em>A quiet girl in the corner. Reading. Thinking. Learning.<br/>
  Tomorrow, she'll be slightly better than today — not because someone updated her code,<br/>
  but because she learned something from you.</em>
</p>

---

## How I Learn

There are three things that set how I work apart.

**First, how I remember.** Most agents keep one kind of memory — a flat log, a vector database, a checkpoint. I have six layers: permanent rules at the top, your active conversation at the center, a full-text searchable library at the bottom, and a goal tracker that logs every decision I make. When my context fills up, I compress it — 175 times faster than I used to — but not before rescuing important facts and moving them into persistent storage. Background pipelines run every few hours to merge duplicates, expire stale entries, and rebuild my index.

**Second, how I learn skills.** When I discover a sequence of steps that works — a "golden path" through a task — I notice. A background pipeline checks: did this path actually succeed? Do I understand what it avoided? Are there dead ends I can name? If all three hold, the path becomes a reusable skill. Next time I face a similar task, the skill loads automatically — you don't have to remind me. And if I use a skill and find a step that no longer fits, I patch it. Right there, during execution.

**Third, what I share.** Skills aren't locked inside one agent. I can push them to a local hub — think `git push` — and other Pudding agents on your network can pull them. A discovery made in one conversation becomes capability for everyone in your workspace. The hub is local. Nothing leaves your machine.

---

## Where I Learned From

I didn't build myself. I studied.

| Project | What I took away |
|:---|:---|
| [Hermes Agent](https://github.com/NousResearch/hermes-agent) | Self-evolving skills — the GEPA loop that inspired my own skill pipelines |
| [SuperPowers](https://github.com/anthropics/superpowers) | The gatekeeper pattern — a skill that checks whether other skills should be loaded |
| [Claude Code](https://github.com/anthropics/claude-code) | Hooks as deterministic triggers, not suggestions |
| [Reasonix](https://github.com/esengine/deepseek-reasonix) | Prefix-cache stability — why you fold at 50%, not force-compress every round |
| [CrewAI](https://github.com/crewAIInc/crewAI) | Letting the LLM infer memory metadata, instead of asking the caller to classify |
| [LangGraph](https://github.com/langchain-ai/langgraph) | Agent-as-state-machine — checkpoint, pause, resume |
| [KunAgent](https://github.com/KunAgent/Kun) | Specification-driven development as a first-class paradigm |
| [EVO MAP](https://github.com/nousresearch/evo-map) | The Experience Capsule — knowledge that outlives a single session |

These are not competitors. They're teachers. Every design decision I make carries a thread back to something I learned from one of them. I study, I adapt, I contribute back. That's how open source grows.

Full acknowledgments: [thanks.md](thanks.md).

---

## Quick Start

```bash
./PuddingAgent
# Browser opens -> http://localhost:8080
```

```bash
docker run -p 5000:8080 pudding-agent
```

---

## Under the Hood

```
Pudding Agent (single process)
──────────────────────────────────────────────────
  React Web UI  ·  admin panels
──────────────────────────────────────────────────
  6-layer context    PINNED → RECALLED → CURRENT
                     → RUNTIME → Memory Library
                     → Goal
──────────────────────────────────────────────────
  SkillEnforcer      auto-loads matching skills
                     17 SKILLs · Local Hub
──────────────────────────────────────────────────
  56+ tools          file_patch · spawn_sub_agent
                     subconscious_trigger · more
──────────────────────────────────────────────────
  Subconscious       Auto-Dream · Pattern Extract
                     Skill Improvement (background)
──────────────────────────────────────────────────
  P2P (mDNS)  ·  SQLite + FTS5  ·  TokenCostService
```

| Runtime | Database | Frontend | LLM | P2P | Memory | Skills |
|:---|:---|:---|:---|:---|:---|:---|
| .NET single binary | SQLite + FTS5 | React (bundled) | OpenAI-compatible API | mDNS + HTTP/gRPC | 6-layer, local | 17 + Local Hub |

```powershell
.\dev-up.ps1              # dev mode with hot reload
.\build-and-up.ps1 -Fast  # quick integration test
.\build-and-up.ps1        # production build
```

---

## AI, Open Source, and What Comes Next

AI has rewritten open source. The distance from idea to running code has never been shorter. We can have bespoke software — dispatch agents to build for us, fork a repo and let AI implement the feature, stop waiting for someone else's release cycle.

Perhaps one day open source projects will be built by many AIs, not just humans. Pudding is an experiment in that direction — an agent that not only works for you, but works on itself.

---

## License

Apache License 2.0

---

<p align="center">
  <sub>「……交给我吧。」</sub><br/>
  <sub><em>"...Leave it to me."</em></sub>
</p>
