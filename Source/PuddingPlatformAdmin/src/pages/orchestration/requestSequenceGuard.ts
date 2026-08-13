/**
 * S2-B6' catalog late-arrival guard (doc 85 §6.2:146).
 *
 * `loadRun` / `loadGraphPreview` issue async catalog/graph requests without
 * an abort or sequencing mechanism. When two navigations overlap (user
 * switches Graph/Run while a previous load is still in flight), the slow
 * response of the older request can resolve *after* the newer one and
 * overwrite the newer Graph's catalog/definition/diagnostic state.
 *
 * This guard hands out strictly increasing request tokens. A loader must
 * call `next()` before issuing its async work, then check `isCurrent(token)`
 * when the response arrives; a token that is no longer the most recent one
 * belongs to a superseded (stale) request and its response must be dropped.
 * The guard is a plain closure with no external dependencies, so it can be
 * unit-tested in isolation.
 */
export interface RequestSequenceGuard {
  /** Issues the next monotonically increasing request token. */
  next(): number;
  /**
   * True when `token` belongs to the most recent request (nothing newer has
   * started since `next()` returned it). False means the request was
   * superseded and its response must be discarded.
   */
  isCurrent(token: number): boolean;
  /** The most recent issued token; 0 when no request has been issued yet. */
  latest(): number;
}

export function createRequestSequenceGuard(): RequestSequenceGuard {
  let current = 0;
  return {
    next: () => ++current,
    // Tokens are only ever issued by next() which starts at 1, so a raw 0
    // (pre-issue sentinel) can never be treated as an applicable response.
    isCurrent: (token) => token > 0 && token === current,
    latest: () => current,
  };
}
