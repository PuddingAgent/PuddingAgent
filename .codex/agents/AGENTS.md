# Codex Agent Roles

This directory is the Codex role copy of `.github/agents`.

## Model Routing

| Work type | Codex model |
| --- | --- |
| Exploration / read-only context gathering | `gpt-5.4-mini` |
| Lead orchestration, planning, architecture, design, QA, security, compliance, user-experience review | `gpt-5.5` |
| Construction / implementation / development | `gpt-5.3-codex` |

## Role Model Map

| Role | Model | Reason |
| --- | --- | --- |
| `lead` | `gpt-5.5` | Leader and orchestration decisions. |
| `pm` | `gpt-5.5` | Task planning and DoR. |
| `architect` | `gpt-5.5` | Architecture decisions and ADRs. |
| `explore` | `gpt-5.4-mini` | Lightweight read-only exploration. |
| `lightweight-developer` | `gpt-5.3-codex` | Simple construction and local implementation. |
| `dev` | `gpt-5.3-codex` | Standard development implementation. |
| `super-dev` | `gpt-5.3-codex` | Complex development implementation. |
| `integration-debugger` | `gpt-5.5` | Root-cause diagnosis and cross-module reasoning. |
| `qa` | `gpt-5.5` | Independent QA review. |
| `security-reviewer` | `gpt-5.5` | Security review. |
| `crypto-evaluation-expert` | `gpt-5.5` | Cryptography/compliance review. |
| `ui-designer` | `gpt-5.5` | UI/UX planning and critique. |
| `user-agent` | `gpt-5.5` | User-experience acceptance critique. |
| `doc` | `gpt-5.4-mini` | Documentation synchronization; escalate if architecture judgment is needed. |

## Notes

- Source files remain in `.github/agents`.
- Codex-specific files use `codex_tools` instead of GitHub/VSC-specific `tools` entries.
- Development handoffs should route implementation to `gpt-5.3-codex` subagents when subagent tooling is available.
- QA must remain independent from the implementation role.
