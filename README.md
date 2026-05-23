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
  <strong>A self-contained, P2P-capable AI agent that runs as a single binary. Double-click to start.</strong><br/>
  <sub><a href="README_zh-CN.md">中文说明</a></sub>
</p>

---

## What Is Pudding?

**Pudding is an AI agent that runs on your machine.**

One binary. Double-click. A browser tab opens. You talk. She works.

She has her own memory (SQLite), her own tools, her own web UI.

When you run multiple Pudding agents, they find each other automatically over the local network — peer-to-peer, no central server. They collaborate like ants: each working on what they can see, leaving traces for others to pick up.

<p align="center">
  <em>A quiet girl in the library corner. Reading. Thinking. Waiting for your task.<br/>
  She doesn't talk much — but she gets things done.</em>
</p>

---

## Why Pudding?

Pudding is different:

- **It's yours.** Runs on your machine. Your data stays with you.
- **It's simple.** One file. No database setup. No infrastructure.
- **It's personal.** She has a face, a name, a memory. She's your agent, not a faceless API.
- **It scales sideways.** More agents join the P2P network when you need them. No orchestration needed.

---

## Quick Start

```bash
# Download the binary for your OS
# Windows: PuddingAgent.exe
# Linux:   PuddingAgent
# macOS:   PuddingAgent

# Run it
./PuddingAgent

# Browser opens automatically -> http://localhost:8080
# That's it. Start talking.
```

### Docker (optional)

```bash
# Port mapping: 5000 (host) → 8080 (container)
docker run -p 5000:8080 pudding-agent
# Browser -> http://localhost:5000
```

---

## Development / Build / Release

Pudding provides three build workflows for different scenarios:

### Daily Development (source watch, HMR, no Docker required)

```powershell
.\dev-up.ps1
```

- Backend `dotnet watch`: auto-restarts on `.cs` file changes
- Frontend `pnpm run start:dev`: HMR on `.tsx`/`.css` changes
- Frontend dev server: http://localhost:8000
- Backend API: http://localhost:5000
- Status: `.\dev-up.ps1 -Status`
- View logs: `.\dev-up.ps1 -Logs`
- Stop: `.\dev-up.ps1 -Down`
- Restart: `.\dev-up.ps1 -Restart`
- Skip dependency install: `.\dev-up.ps1 -NoInstall`

### Fast Integration (verify local publish + container)

```powershell
.\build-and-up.ps1 -Fast
```

Optional skip switches:

```powershell
.\build-and-up.ps1 -Fast -SkipFrontend    # skip frontend build
.\build-and-up.ps1 -Fast -SkipBackend     # skip backend publish
.\build-and-up.ps1 -Fast -NoInstall       # skip pnpm install
```

### Release Verification (production build + image)

```powershell
.\build-and-up.ps1
```

Runs frontend production build → Docker `dotnet publish` → final image.

---

## Tech Stack

She's built with one rule: **zero external dependencies for the user.**

| What     | How                                      |
| :------- | :--------------------------------------- |
| Runtime  | .NET (ASP.NET Core, single binary)       |
| Database | SQLite — one file, auto-created          |
| Web UI   | React, bundled inside the binary         |
| LLM      | Direct API call (OpenAI-compatible)      |
| P2P      | mDNS discovery + direct HTTP/gRPC        |
| Memory   | Local, persistent, private               |

---

## Architecture

```
┌──────────────────────────────────────┐
│        Pudding Agent (1 process)      │
│                                       │
│  Browser → localhost:8080             │
│  ┌─────────────────────────────────┐ │
│  │        Web UI (React)           │ │
│  ├─────────────────────────────────┤ │
│  │     Controller (routing/auth)   │ │
│  ├─────────────────────────────────┤ │
│  │     Runtime (LLM/tools/memory)  │ │
│  ├─────────────────────────────────┤ │
│  │     P2P Network Layer           │ │
│  ├─────────────────────────────────┤ │
│  │     SQLite                      │ │
│  └─────────────────────────────────┘ │
│                                       │
│  ← P2P → other Pudding agents         │
└──────────────────────────────────────┘
```

Read the full architecture: [Docs/架构.md](Docs/架构.md)

---

## The Name & The Face

She's called **Pudding** (布丁). Quiet. Efficient. A little mysterious.

You give her a task. She tilts her head slightly. A few seconds pass. "Done."

She keeps her own notebook (SQLite). She remembers you. She doesn't share your secrets with the cloud. She just works — from your desktop, your server, or a Raspberry Pi in the corner.

Her design is inspired by the quiet capability of a certain library-dwelling literary girl. She reads. She understands. She acts. No fanfare. Just results.

---

## Multi-Agent: The Ant Colony

When you run more than one Pudding agent on your network:

1. They discover each other automatically (mDNS)
2. They talk directly (no central broker)
3. They share task events (P2P broadcast)
4. They divide work like ants — each doing what fits their role and skills

This is not orchestration. This is emergence.

---

## AI, Open Source, and the New Game

AI has fundamentally rewritten the rules of open source. In the past, monumental projects like Linux and ffmpeg were guided by brilliant programmers. Today, AI is gradually taking over what once required years of experience — and more and more ordinary people can now participate in building open source.

The cost of starting an open source project is dropping — but so might be its quality. It's hard to judge whether this is good or bad. Perhaps it's just the growing pains of a new era.

But here's the good part: now, if you have an idea in your head, AI can bring it to life for you — no more spending countless hours writing code line by line. **The distance from idea to reality has never been shorter.**

And here's something even more interesting: AI changes the very nature of open source participation. We can now have our own bespoke software. We can dispatch AI agents to build software *for us*, rather than waiting for OpenClaw or Hermes-Agent to ship the features we need. When we want a feature, we can just fork the repo and let AI implement it — no more waiting for the original author. Perhaps, in the future, open source projects will be built by many, many AIs, not just humans.

On that note, **[EVO MAP](https://github.com/nousresearch/evo-map)** introduced a fascinating concept: the **Experience Capsule** (经验胶囊). It's a brilliant idea — like Doraemon's "Memory Bread" — packaging learned knowledge into portable, reusable capsules that other agents can consume. This is the kind of thinking that inspires Pudding's memory system.

This project was born from my exploration of various AI tools. It draws ideas from many excellent projects and products — not copying, but standing on the shoulders of those who came before, asking: *"What should the next generation of AI agents look like?"*

---

## Acknowledgments

Pudding's design is deeply inspired by the following projects and research:

- **[Claude Code](https://github.com/anthropics/claude-code)** (Anthropic) — Tool interface design, permission pipeline, Coordinator/Worker patterns
- **[Hermes-Agent](https://github.com/NousResearch/hermes-agent)** (Nous Research) — Self-registering plugin architecture, memory provider pattern, multi-platform routing
- **[OpenCode](https://github.com/anomalyco/opencode)** — Bottleneck analysis of structured code understanding
- **[Cursor](https://cursor.com/)** — Product experience of AI-powered code editors
- **[OpenHarness](https://github.com/anthropics/openharness)** — Harness loop, Hook system, 5-level security boundaries
- **[OpenClaw](https://github.com/anthropics/openclaw)** — Memory system, multi-channel Gateway architecture
- **[OpenHanako](https://github.com/liliMozi/openhanako)** — Multi-tier memory pipeline (today/week/longterm/facts), sandbox security, plugin hot-swap
- **[SuperPowers](https://github.com/anthropics/superpowers)** — Skill system, TDD discipline, subagent-driven development
- **[EVO MAP](https://github.com/nousresearch/evo-map)** — Experience Capsule concept, portable knowledge packaging
- **[Strange Loop Canon](https://www.strangeloopcanon.com/)** (Rohit Krishnan et al.) — Multi-agent coordination mechanisms, information drift, shared Board research
- **[Strange Loop Canon Archive](https://www.strangeloopcanon.com/archive)** — Comprehensive archive of agent economy experiments
- **[Building Effective Agents](https://www.anthropic.com/engineering/building-effective-agents)** (Anthropic) — Agent design patterns guide
- **[AutoGPT](https://github.com/Significant-Gravitas/AutoGPT)** / **[BabyAGI](https://github.com/yoheinakajima/babyagi)** — Early pioneers of autonomous agents
- **[LangChain](https://github.com/langchain-ai/langchain)** / **[CrewAI](https://github.com/crewAIInc/crewAI)** — Agent framework engineering practices

And to all the open source contributors and researchers exploring the frontier of AI agents.

---

## License

Apache License 2.0

---

<p align="center">
  <sub>「……交给我吧。」</sub><br/>
  <sub><em>"...Leave it to me."</em></sub>
</p>