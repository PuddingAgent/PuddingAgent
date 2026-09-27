# Agent chat application projections

- `AgentMainSessionService`: Core-owned native role main-session creation, redirect resolution and manifest binding; serialized creation, canonical session repository.
- `AgentConversationProjectionService`: canonical conversation/recent activity/process-details projections, direct `ISessionRepository` lookup (no self HTTP).
- `AgentRunProjectionService`: role run status/unread projection, same direct session repository.

Desktop Composition consumes these application services in fresh scopes. Web controllers continue to consume the same projection interfaces. UI selection/drafts and WinUI types never enter this directory.
