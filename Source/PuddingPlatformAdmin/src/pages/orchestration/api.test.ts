import { encodeRevisionPath, parseSseChunk } from './api';

describe('orchestration SSE client', () => {
  it('keeps revision slashes as route separators while escaping every segment', () => {
    expect(encodeRevisionPath('graph A/rev#2')).toBe('graph%20A/rev%232');
  });

  it('parses split canonical frames and ignores heartbeat comments', () => {
    const first = parseSseChunk('', ': heartbeat\n\nid: 7\nevent: orchestration.node.');
    expect(first.frames).toEqual([]);

    const second = parseSseChunk(
      first.remainder,
      'started\ndata: {"runId":"run-1","eventType":"orchestration.node.started","sequence":7}\n\n',
    );
    expect(second.remainder).toBe('');
    expect(second.frames).toEqual([
      expect.objectContaining({
        id: '7',
        event: 'orchestration.node.started',
        data: expect.objectContaining({ runId: 'run-1', sequence: 7 }),
      }),
    ]);
  });

  it('isolates malformed JSON without dropping the following event', () => {
    const parsed = parseSseChunk(
      '',
      'event: bad\ndata: {nope}\n\nid: 8\nevent: good\ndata: {"sequence":8}\n\n',
    );
    expect(parsed.frames).toHaveLength(1);
    expect(parsed.frames[0]).toEqual(expect.objectContaining({ id: '8', event: 'good' }));
  });

});
