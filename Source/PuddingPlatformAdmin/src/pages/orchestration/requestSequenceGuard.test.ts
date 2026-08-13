import { createRequestSequenceGuard } from './requestSequenceGuard';

describe('S2-B6\' catalog request-sequence guard', () => {
  it('issues strictly increasing tokens', () => {
    const guard = createRequestSequenceGuard();
    const first = guard.next();
    const second = guard.next();
    const third = guard.next();
    expect(second).toBe(first + 1);
    expect(third).toBe(second + 1);
    expect(guard.latest()).toBe(third);
  });

  it('drops a late response whose token was superseded by a newer request', () => {
    const guard = createRequestSequenceGuard();
    const staleToken = guard.next(); // request A issued
    const currentToken = guard.next(); // request B supersedes A
    // A's response arrives after B started -> old sequence -> discard.
    expect(guard.isCurrent(staleToken)).toBe(false);
    // B's response is still the latest -> may be applied.
    expect(guard.isCurrent(currentToken)).toBe(true);
  });

  it('keeps a token current until a newer request supersedes it', () => {
    const guard = createRequestSequenceGuard();
    const token = guard.next();
    expect(guard.isCurrent(token)).toBe(true);
    guard.next();
    expect(guard.isCurrent(token)).toBe(false);
  });

  it('starts at 0 before any request is issued', () => {
    const guard = createRequestSequenceGuard();
    expect(guard.latest()).toBe(0);
    // Token 0 was never issued, so it can never be current.
    expect(guard.isCurrent(0)).toBe(false);
  });
});
