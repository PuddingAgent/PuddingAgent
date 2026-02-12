interface AgentContactClickState {
  clickedAgentId?: string;
  currentAgentId?: string;
  selectedSessionId?: string | null;
  mainSessionId?: string | null;
  turnCount: number;
}

export function shouldIgnoreAgentContactClick(
  state: AgentContactClickState,
): boolean {
  if (!state.clickedAgentId || state.clickedAgentId !== state.currentAgentId)
    return false;
  if (state.turnCount <= 0) return false;
  return Boolean(
    state.mainSessionId && state.selectedSessionId === state.mainSessionId,
  );
}
