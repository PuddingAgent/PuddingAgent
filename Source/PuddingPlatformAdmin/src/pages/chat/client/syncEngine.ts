export interface AgentChatEventPage {
  events: unknown[];
  nextCursor: number;
}

export function createAgentChatSyncEngine(input: {
  fetchEvents(
    workspaceId: string,
    agentId: string,
    after: number,
  ): Promise<AgentChatEventPage>;
  applyEvents(events: unknown[]): void;
}) {
  return {
    async replay(workspaceId: string, agentId: string, after: number) {
      const page = await input.fetchEvents(workspaceId, agentId, after);
      input.applyEvents(page.events);
      return page.nextCursor;
    },
  };
}
