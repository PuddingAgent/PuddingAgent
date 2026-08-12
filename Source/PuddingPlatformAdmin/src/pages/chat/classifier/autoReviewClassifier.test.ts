// ── P2#9 autoReviewClassifier 纯逻辑测试 ─────────────────
import {
  addRecentlyDeniedItem,
  applyAutoReviewBlock,
  AUTO_REVIEW_BLOCK_CONSECUTIVE_THRESHOLD,
  AUTO_REVIEW_BLOCK_TOTAL_THRESHOLD,
  createInitialAutoReviewState,
  disableAutoReview,
  removeRecentlyDeniedItem,
  resetAutoReviewState,
  retryRecentlyDeniedItem,
  shouldFallbackToManual,
  toRecentlyDeniedItem,
  type AutoReviewClassifierState,
  type RecentlyDeniedItem,
} from './autoReviewClassifier';

describe('autoReviewClassifier', () => {
  const makeState = (
    overrides: Partial<AutoReviewClassifierState> = {},
  ): AutoReviewClassifierState => ({
    ...createInitialAutoReviewState(),
    ...overrides,
  });

  const makeBlock = (
    overrides: Partial<RecentlyDeniedItem> = {},
  ): Omit<RecentlyDeniedItem, 'id' | 'blockedAt'> => ({
    toolName: 'shell',
    description: 'rm -rf /tmp/build',
    riskLevel: 'high',
    rule: 'destructive',
    source: 'classifier',
    ...overrides,
  });

  describe('shouldFallbackToManual', () => {
    it('returns null below both thresholds', () => {
      expect(
        shouldFallbackToManual({ consecutiveBlocks: 2, totalBlocks: 19 }),
      ).toBeNull();
    });

    it('returns consecutive when consecutive threshold reached', () => {
      expect(
        shouldFallbackToManual({
          consecutiveBlocks: AUTO_REVIEW_BLOCK_CONSECUTIVE_THRESHOLD,
          totalBlocks: 1,
        }),
      ).toBe('consecutive');
    });

    it('returns total when total threshold reached even with low consecutive', () => {
      expect(
        shouldFallbackToManual({
          consecutiveBlocks: 0,
          totalBlocks: AUTO_REVIEW_BLOCK_TOTAL_THRESHOLD,
        }),
      ).toBe('total');
    });
  });

  describe('applyAutoReviewBlock', () => {
    it('increments consecutive and total counters', () => {
      const next = applyAutoReviewBlock(makeState(), makeBlock());
      expect(next.consecutiveBlocks).toBe(1);
      expect(next.totalBlocks).toBe(1);
      expect(next.fallbackTriggered).toBe(false);
    });

    it('triggers fallback on 3 consecutive blocks', () => {
      let state = makeState();
      for (let i = 0; i < AUTO_REVIEW_BLOCK_CONSECUTIVE_THRESHOLD; i += 1) {
        state = applyAutoReviewBlock(state, makeBlock());
      }
      expect(state.fallbackTriggered).toBe(true);
      expect(state.fallbackReason).toBe('consecutive');
      expect(state.lastBlockRule).toBe('destructive');
    });

    it('triggers fallback on 20 cumulative blocks even with resets between', () => {
      let state = makeState();
      for (let i = 0; i < AUTO_REVIEW_BLOCK_TOTAL_THRESHOLD; i += 1) {
        state = applyAutoReviewBlock(state, makeBlock());
        if (i % 3 === 2) {
          state = resetAutoReviewState(state); // 模拟中间恢复 auto 清空连续计数
        }
      }
      expect(state.fallbackTriggered).toBe(true);
      expect(state.fallbackReason).toBe('total');
    });

    it('records lastBlockedAt and rule', () => {
      const now = 123456789;
      const next = applyAutoReviewBlock(
        makeState(),
        makeBlock({ blockedAt: now }),
      );
      expect(next.lastBlockedAt).toBe(now);
      expect(next.lastBlockRule).toBe('destructive');
    });
  });

  describe('resetAutoReviewState', () => {
    it('clears consecutive counters and fallback but keeps enabled and totalBlocks', () => {
      let state = applyAutoReviewBlock(makeState(), makeBlock());
      state = applyAutoReviewBlock(state, makeBlock());
      state = applyAutoReviewBlock(state, makeBlock());
      expect(state.fallbackTriggered).toBe(true);

      const next = resetAutoReviewState(state);
      expect(next.consecutiveBlocks).toBe(0);
      // 累计计数保留（"累计 20 次" 为会话级累计）
      expect(next.totalBlocks).toBe(3);
      expect(next.fallbackTriggered).toBe(false);
      expect(next.fallbackReason).toBeNull();
      expect(next.enabled).toBe(true);
    });
  });

  describe('disableAutoReview', () => {
    it('sets enabled false and preserves counters', () => {
      const state = applyAutoReviewBlock(makeState(), makeBlock());
      const next = disableAutoReview(state);
      expect(next.enabled).toBe(false);
      expect(next.totalBlocks).toBe(1);
    });
  });

  describe('recently denied list helpers', () => {
    const makeItem = (id: string, source: RecentlyDeniedItem['source'] = 'classifier'): RecentlyDeniedItem =>
      toRecentlyDeniedItem({ ...makeBlock(), id, source });

    it('adds at head and dedupes by id', () => {
      const a = makeItem('a');
      const b = makeItem('b');
      let list = addRecentlyDeniedItem([], a);
      list = addRecentlyDeniedItem(list, b);
      expect(list.map((i) => i.id)).toEqual(['b', 'a']);
      list = addRecentlyDeniedItem(list, a);
      expect(list.map((i) => i.id)).toEqual(['a', 'b']);
    });

    it('caps the list at maxItems', () => {
      let list: RecentlyDeniedItem[] = [];
      for (let i = 0; i < 25; i += 1) {
        list = addRecentlyDeniedItem(list, makeItem(`id-${i}`), 20);
      }
      expect(list.length).toBe(20);
      expect(list[0].id).toBe('id-24');
    });

    it('removes an item by id', () => {
      const list = [makeItem('a'), makeItem('b')];
      const next = removeRecentlyDeniedItem(list, 'a');
      expect(next.map((i) => i.id)).toEqual(['b']);
    });

    it('retry returns item and removes it from the list', () => {
      const list = [makeItem('a'), makeItem('b')];
      const result = retryRecentlyDeniedItem(list, 'a');
      expect(result.item?.id).toBe('a');
      expect(result.list.map((i) => i.id)).toEqual(['b']);
    });

    it('retry of unknown id returns undefined and unchanged list', () => {
      const list = [makeItem('a')];
      const result = retryRecentlyDeniedItem(list, 'zzz');
      expect(result.item).toBeUndefined();
      expect(result.list).toHaveLength(1);
    });
  });

  describe('toRecentlyDeniedItem', () => {
    it('fills default source and id', () => {
      const item = toRecentlyDeniedItem(makeBlock());
      expect(item.source).toBe('classifier');
      expect(item.id).toMatch(/^ar-/);
      expect(Number.isFinite(item.blockedAt)).toBe(true);
    });

    it('preserves explicit id/source/blockedAt', () => {
      const item = toRecentlyDeniedItem(
        makeBlock({ id: 'custom-id', source: 'user_deny', blockedAt: 42 }),
      );
      expect(item.id).toBe('custom-id');
      expect(item.source).toBe('user_deny');
      expect(item.blockedAt).toBe(42);
    });
  });
});
