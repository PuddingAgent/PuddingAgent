import {
  readReasoningEffort,
  reasoningEffortStorageKey,
  writeReasoningEffort,
} from './reasoningEffortPreference';

describe('reasoningEffortPreference', () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it('isolates saved effort per agent', () => {
    writeReasoningEffort('agent-a', 'max');
    writeReasoningEffort('agent-b', 'high');

    expect(readReasoningEffort('agent-a')).toBe('max');
    expect(readReasoningEffort('agent-b')).toBe('high');
  });

  it('returns undefined when the agent has no saved value', () => {
    expect(readReasoningEffort('agent-unknown')).toBeUndefined();
    expect(readReasoningEffort(undefined)).toBeUndefined();
  });

  it('treats invalid stored values as absent', () => {
    // medium 是历史虚构档位，服务商无此值；读到它必须当作没存过，回落默认 high。
    window.localStorage.setItem(reasoningEffortStorageKey('agent-a'), 'medium');

    expect(readReasoningEffort('agent-a')).toBeUndefined();
  });

  it('survives a storage that throws', () => {
    // 用 Storage.prototype 注入故障：直接给实例属性赋值在 jsdom 下不一定生效。
    const spy = jest
      .spyOn(Storage.prototype, 'setItem')
      .mockImplementation(() => {
        throw new Error('quota exceeded');
      });

    expect(() => writeReasoningEffort('agent-a', 'max')).not.toThrow();

    spy.mockRestore();
    expect(readReasoningEffort('agent-a')).toBeUndefined();
  });
});
