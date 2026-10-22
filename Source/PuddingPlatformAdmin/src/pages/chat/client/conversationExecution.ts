import type { AgentConversationView } from './types';

/** The same scoped snapshot owns both the transcript and its execution controls. */
export function resolveConversationExecution(
  conversation: AgentConversationView | null | undefined,
  workspaceId: string | undefined,
  agentId: string | undefined,
  sessionId: string | null | undefined,
): { running: boolean; turnId: string | null } | undefined {
  if (!conversation || conversation.workspaceId !== workspaceId ||
      conversation.agentId !== agentId || conversation.mainSessionId !== sessionId) {
    return undefined;
  }
  const run = conversation.activeRun;
  const running = Boolean(run && ['queued', 'running', 'waiting'].includes(run.status));
  return {
    running,
    // A run ID is not a Turn ID. Missing canonical identity must fail closed.
    turnId: running ? run?.outputSnapshot.window?.turnId ?? null : null,
  };
}
