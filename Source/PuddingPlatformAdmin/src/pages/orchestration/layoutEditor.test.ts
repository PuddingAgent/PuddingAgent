import {
  buildOrchestrationLayoutWrite,
  getOrchestrationLayoutConflict,
} from './layoutEditor';

describe('orchestration layout editor', () => {
  const nodes = [
    { id: 'research', position: { x: 120, y: 80 } },
    { id: 'proposal', position: { x: 460, y: 210 } },
  ];

  it('creates the first layout at revision one', () => {
    const write = buildOrchestrationLayoutWrite({
      graphId: 'graph-1',
      baseRevisionId: 'graph-1/rev-1',
      currentLayout: undefined,
      nodes,
      viewport: { x: 15, y: 25, zoom: 1.25 },
    });

    expect(write.expectedCurrentLayoutRevision).toBe(0);
    expect(write.layout.layoutRevision).toBe(1);
    expect(write.layout.viewport).toEqual({ x: 15, y: 25, zoom: 1.25 });
    expect(write.layout.nodes).toEqual([
      { nodeId: 'research', x: 120, y: 80, collapsed: false },
      { nodeId: 'proposal', x: 460, y: 210, collapsed: false },
    ]);
  });

  it('advances CAS and preserves layout metadata that the current slice does not edit', () => {
    const write = buildOrchestrationLayoutWrite({
      graphId: 'graph-1',
      baseRevisionId: 'graph-1/rev-1',
      currentLayout: {
        graphId: 'graph-1',
        baseRevisionId: 'graph-1/rev-1',
        layoutRevision: 7,
        viewport: { x: 0, y: 0, zoom: 1 },
        nodes: [
          {
            nodeId: 'research',
            x: 0,
            y: 0,
            width: 280,
            height: 96,
            parentNodeId: 'group-1',
            collapsed: true,
          },
        ],
      },
      nodes,
      viewport: { x: -30, y: 40, zoom: 0.9 },
    });

    expect(write.expectedCurrentLayoutRevision).toBe(7);
    expect(write.layout.layoutRevision).toBe(8);
    expect(write.layout.nodes[0]).toEqual({
      nodeId: 'research',
      x: 120,
      y: 80,
      width: 280,
      height: 96,
      parentNodeId: 'group-1',
      collapsed: true,
    });
  });

  it('extracts a 409 CAS conflict without treating other failures as conflicts', () => {
    expect(
      getOrchestrationLayoutConflict({
        response: { status: 409 },
        data: {
          message: 'layout changed',
          currentLayoutRevision: 9,
        },
      }),
    ).toEqual({ message: 'layout changed', currentLayoutRevision: 9 });
    expect(
      getOrchestrationLayoutConflict({ response: { status: 500 } }),
    ).toBeUndefined();
  });
});
