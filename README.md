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
- Reverse proxy: http://localhost/ locally, or `http://<host-lan-ip>/` from other LAN devices
- Status: `.\dev-up.ps1 -Status`
- View logs: `.\dev-up.ps1 -Logs`
- Stop: `.\dev-up.ps1 -Down`
- Restart: `.\dev-up.ps1 -Restart`
- Rebuild then start: `.\dev-up.ps1 -Rebuild`
- Restart and rebuild: `.\dev-up.ps1 -Restart -Rebuild`
- Skip dependency install: `.\dev-up.ps1 -NoInstall`

### Local Diagnostics

Machine-readable diagnostics are kept separate from human-readable logs:

- Backend session timeline: `data/logs/diagnostics/session-timeline/YYYYMMDD/{sessionId}.jsonl`
- Reverse proxy SSE/replay timeline: `data/logs/diagnostics/proxy/YYYYMMDD.jsonl`
- SQLite event tables: `data/pudding_platform.db`
- Aggregated telemetry facts: `telemetry_metric_events` in `data/pudding_platform.db`
- Context layer metrics: `context_layer_metric_events` in `data/pudding_platform.db`

Observability is treated as a product foundation, not only as a troubleshooting log. New runtime, tool, approval, memory, sub-agent, benchmark, and frontend flows should produce both replayable evidence and structured metrics.

The observability model has three layers:

- **Trace evidence**: JSONL timelines, approval audit events, session logs, proxy diagnostics, and exported bundles. These answer why one specific run failed or slowed down.
- **Metrics facts**: quantitative, aggregatable rows such as tool duration, output size, approval path, implicit approval latency, context layer token share, layer cache hit rate, memory recall count, and benchmark run quality. These primarily live in `telemetry_metric_events` and dedicated metric tables.
- **Insights**: derived summaries for dashboards and scripts, such as implicit approval coverage, oversized tool-output rate, retry rate, recovery rate, benchmark score trend, and sub-agent contribution.

The pipeline has three stages:

1. **Collection**: capture stable, typed fields at the business event, not only prose logs.
2. **Cleaning and processing**: classify failures, redact and truncate previews, hash sensitive payloads, apply thresholds, and compute derived metrics.
3. **Presentation**: expose the results through the admin diagnostics UI, `Tools/Diagnostics`, and `TestScripts` scripts.

Standalone Python tools live in `Tools/Diagnostics` and use the repo `.venv`:

```powershell
.\.venv\Scripts\python.exe Tools\Diagnostics\query_timeline.py --session-id <sessionId>
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py telemetry-summary --days 7
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py tool-usage --session-id <sessionId>
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py tool-output --days 7 --min-chars 8192
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py context-layers --days 30
.\.venv\Scripts\python.exe Tools\Diagnostics\inspect_schema.py
.\.venv\Scripts\python.exe Tools\Diagnostics\export_session_bundle.py --session-id <sessionId>
```

`/api/stats/tokens/context-layers` and `query_metrics.py context-layers` expose context composition metrics for cache optimization: layer token shares, average and median estimated cache hit rates, layer volatility, change reasons, and P95 token size.

Diagnostics timeline recording is enabled by default in development. Override it with `PUDDING_DIAGNOSTICS_TIMELINE=0` or `PUDDING_DIAGNOSTICS_TIMELINE=1`.
High-detail telemetry previews are disabled by default. Set the unified switch `PUDDING_DEBUG=1` (legacy `PUDDING_TELEMETRY_DEBUG=1` is still honored) only during local debugging to store redacted/truncated context and tool previews in telemetry `debug_json`. `dev-up.py` injects both switches for local development.

### Context Cache And Input Compression

Pudding treats token-cost control as two related layers:

- **Cache-hit observability**: `prompt_cache_hit_tokens` / `prompt_cache_miss_tokens`, `telemetry_metric_events`, and `context_layer_metric_events` track cache hit rate, context-layer volatility, and cost attribution for each LLM call.
- **Pre-LLM input compression**: the design adds a local compression layer for tool outputs, logs, file/search/diff excerpts, and RAG chunks before they reach the LLM. User messages, system-prompt meaning, recent turns, and the active execution turn are protected by default.

We evaluated [Headroom](https://github.com/chopratejas/headroom) as a reference design. Its content routing, prefix stabilization, and CCR (Compress-Cache-Retrieve) retrieval model match Pudding's goals for lowering input tokens and improving provider prefix-cache hit opportunities. Pudding will keep the default path native through `ContextInputCompression` so compression, permissions, SQLite storage, diagnostic bundles, and cache metrics stay under one governance boundary. Headroom remains useful as a development benchmark, optional proxy/provider, and implementation reference.

Related design docs:

- [ADR-042 Context Auto-Compaction And Compact Command](Docs/07架构/43ADR-042上下文自动压缩与主动Compact命令ADR.md)
- [Context Auto-Compaction Design](Docs/Features/上下文自动压缩与Compact命令设计方案.md)
- [ADR-018 Context Cache Observability](Docs/07架构/18上下文缓存可观测性ADR.md)
- [ADR-043 DeepSeek Context Cache Statistics Loop](Docs/07架构/44ADR-043缓存统计闭环ADR.md)

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

See [thanks.md](thanks.md).

---

## License

Apache License 2.0

---

<p align="center">
  <sub>「……交给我吧。」</sub><br/>
  <sub><em>"...Leave it to me."</em></sub>
</p>
