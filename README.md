# Pudding Agent

<p align="center">
  <img src="me.png" alt="Pudding" width="200"/>
</p>

<p align="center">
  <strong>你好，我是布丁。你的 AI 代理。</strong><br/>
  <sub>Hi. I'm Pudding. Your AI agent.</sub>
</p>

---

**A self-contained, P2P-capable AI agent that runs as a single binary. Double-click to start.**

[中文说明](README_zh-CN.md)

![Version](https://img.shields.io/badge/version-v0.1.0-blue)
![Platform](https://img.shields.io/badge/platform-windows%20%7C%20linux%20%7C%20macos-lightgrey)
![License](https://img.shields.io/badge/license-Apache%202.0-green)

---

## What Is Pudding?

Pudding is not a platform. Not a framework. Not a service mesh.

**Pudding is an AI agent that runs on your machine.**

One binary. Double-click. A browser tab opens. You talk. She works.

She has her own memory (SQLite), her own tools, her own web UI. She doesn't need PostgreSQL. She doesn't need Redis. She doesn't need RabbitMQ.

When you run multiple Pudding agents, they find each other automatically over the local network — peer-to-peer, no central server. They collaborate like ants: each working on what they can see, leaving traces for others to pick up.

<p align="center">
  <em>A quiet girl in the library corner. Reading. Thinking. Waiting for your task.<br/>
  She doesn't talk much — but she gets things done.</em>
</p>

---

## Why Pudding?

Most AI tools today are either cloud services that own your data, or complex platforms that need a dozen Docker containers before you can say "hello."

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
docker run -p 8080:8080 pudding-agent
```

---

## Tech Stack

She's built with one rule: **zero external dependencies for the user.**

| What | How |
|---|---|
| Runtime | .NET (ASP.NET Core, single binary) |
| Database | SQLite — one file, auto-created |
| Web UI | React, bundled inside the binary |
| LLM | Direct API call (OpenAI-compatible) |
| P2P | mDNS discovery + direct HTTP/gRPC |
| Memory | Local, persistent, private |

---

## Architecture

```
+--------------------------------------+
|         Pudding Agent (1 process)     |
|                                       |
|  Browser -> localhost:8080             |
|  +---------------------------------+ |
|  |        Web UI (React)           | |
|  +---------------------------------+ |
|  |     Controller (routing/auth)   | |
|  +---------------------------------+ |
|  |     Runtime (LLM/tools/memory)  | |
|  +---------------------------------+ |
|  |     P2P Network Layer           | |
|  +---------------------------------+ |
|  |     SQLite                      | |
|  +---------------------------------+ |
|                                       |
|  <- P2P -> other Pudding agents         |
+--------------------------------------+
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

## License

Apache License 2.0

---

<p align="center">
  <sub>「……交给我吧。」</sub><br/>
  <sub><em>"...Leave it to me."</em></sub>
</p>