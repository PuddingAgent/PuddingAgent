# PuddingCode

**An agentic self-programming CLI tool.** Talk to it in natural language, it uses LLM tool-calling to read/write files and run shell commands on your project.

![Version](https://img.shields.io/badge/version-v0.1.0-blue)
![Platform](https://img.shields.io/badge/platform-windows%20%7C%20linux%20%7C%20macos-lightgrey)
![License](https://img.shields.io/badge/license-MIT-green)

---

## 🍮 What Is This?

PuddingCode is a CLI assistant that acts on your codebase. Describe what you want, and it will read files, write code, and run shell commands — tool by tool, with safety guards at every step.

## Features

- **Spirit Agent** — Conversational AI assistant that reads/writes files and runs shell commands via tool-calling
- **Swarm Mode** — Leader-Worker multi-agent architecture: Leader defines contracts, spawns Workers that implement code in parallel within scoped file boundaries
- **Git Snapshots** — Auto-snapshots before every tool execution, `/undo` to roll back
- **Multi-provider LLM** — OpenAI, DeepSeek, Claude (via proxy), or any custom OpenAI-compatible endpoint
- **Slash Command REPL** — `/help`, `/open`, `/model`, `/undo`, `/swarm`, `/exit` and more
- **Permission Guard** — File operations sandboxed to project root
- **Skill Registry** — Role-filtered tools per agent role (Leader/Worker/Spirit)

---

## Quick Start

### Build

```bash
dotnet build
```

### Run

```bash
dotnet run --project Source/PuddingCodeCLI
```

Or publish a self-contained binary:

```bash
dotnet publish Source/PuddingCodeCLI -c Release -r win-x64 --self-contained
```

---

## Configuration

PuddingCode stores config in your user config file. On first run it walks you through provider setup interactively.

You can also skip the wizard entirely with environment variables:

| Env Var | Description |
|---------|-------------|
| `PUDDING_API_KEY` | Your LLM API key |
| `PUDDING_API_ENDPOINT` | API base URL (default: `https://api.openai.com/v1`) |
| `PUDDING_MODEL` | Model name (default: `gpt-4o`) |

```bash
PUDDING_API_KEY=sk-... PUDDING_MODEL=gpt-4o dotnet run --project Source/PuddingCodeCLI
```

Supported provider templates out of the box: **OpenAI**, **DeepSeek**, **Claude (via proxy)**, **Custom endpoint**.

---

## Slash Commands

| Command | Description |
|---------|-------------|
| `/help` | Show all commands |
| `/open [path]` | Open a project directory (default: current dir) |
| `/model` | List configured providers |
| `/model add` | Add a new LLM provider |
| `/model use <id>` | Switch active provider |
| `/model remove <id>` | Remove a provider |
| `/undo [N]` | Undo last N tool snapshots (default: 1) |
| `/snapshot [label]` | Create a manual snapshot |
| `/history` | List recent snapshots |
| `/config` | Show current provider details |
| `/swarm` | Swarm mode commands |
| `/swarm status` | View active swarm status |
| `/swarm cancel` | Cancel active swarm and cleanup |
| `/exit` | Exit PuddingCode |

---

## Swarm Mode

Swarm Mode enables parallel code generation with a Leader-Worker architecture.

### How It Works

1. **Leader Agent** receives your task, designs contracts (interfaces + empty implementations with method signatures), and splits the work into scoped modules
2. **Worker Agents** are spawned, each assigned to specific files or symbols — they can only touch their assigned scope
3. **ContractManager** and **WorkerManager** coordinate the flow, validate implementations against contracts, and merge results

```
/swarm start
> "Create a REST API with controllers for Users and Products"

Leader  → defines IUserRepository, IProductRepository, empty controllers
Workers → implement UserController, ProductController, services in parallel
```

### Architecture

```
SwarmOrchestrator
├── ContractManager    # Defines and validates contracts
├── WorkerManager      # Spawns, scopes, and dismisses Worker agents
└── Leader Agent       # Plans, assigns tasks, monitors progress
    └── Worker Agents  # Scoped implementers (one per file/module)
```

**Phase 1** (current): Local serial/parallel swarm  
**Phase 2** (planned): Git worktree-based parallel execution + integrated test runner  
**Phase 3** (planned): P2P distributed swarm across machines

---

## Project Structure

```
Source/
  PuddingCode/           # Core library
    Core/                # AgentOrchestrator, LLM gateway, Git snapshots
    Swarm/               # SwarmOrchestrator, ContractManager, WorkerManager
    Skills/              # Skill registry (role-filtered tools)
    Tools/               # FileTool, ShellTool
    Abstractions/        # Interfaces
    Models/              # Shared models
  PuddingCodeCLI/        # CLI entry point
  PuddingCodeCLITests/
  PuddingCodeTests/
```

---

## License

MIT

🍮
