import { summarizeError } from './summarizeError';

describe('summarizeError', () => {
  it('returns empty summary and full for empty input', () => {
    expect(summarizeError(undefined)).toEqual({ summary: '', full: '' });
    expect(summarizeError(null)).toEqual({ summary: '', full: '' });
    expect(summarizeError('')).toEqual({ summary: '', full: '' });
    expect(summarizeError('   ')).toEqual({ summary: '', full: '' });
  });

  it('extracts .message from JSON and keeps the raw JSON as full', () => {
    const raw = '{"message":"模型超时，即将重试","attempt":3}';
    expect(summarizeError(raw)).toEqual({
      summary: '模型超时，即将重试',
      full: raw,
    });
  });

  it('falls back to .error when .message is missing', () => {
    const raw = '{"error":"连接被拒绝","code":500}';
    const result = summarizeError(raw);
    expect(result.summary).toBe('连接被拒绝');
    expect(result.full).toBe(raw);
  });

  it('prefers .message over .error when both exist', () => {
    expect(summarizeError('{"message":"m1","error":"e1"}').summary).toBe('m1');
  });

  it('ignores non-string message/error fields', () => {
    const raw = '{"message":{"text":"nested"},"error":42}';
    const result = summarizeError(raw);
    expect(result.summary).toBe(raw);
  });

  it('truncates long plain text to max chars with an ellipsis', () => {
    const long = 'x'.repeat(100);
    const result = summarizeError(long);
    expect(result.summary).toBe(`${'x'.repeat(80)}…`);
    expect(result.full).toBe(long);
  });

  it('truncates a long JSON message to max chars with an ellipsis', () => {
    const raw = JSON.stringify({ message: 'y'.repeat(100) });
    const result = summarizeError(raw);
    expect(result.summary).toBe(`${'y'.repeat(80)}…`);
    expect(result.full).toBe(raw);
  });

  it('does not truncate text at or below max', () => {
    expect(summarizeError('short error').summary).toBe('short error');
  });

  it('honors a custom max', () => {
    const result = summarizeError('abcdefghij', 5);
    expect(result.summary).toBe('abcde…');
    expect(result.full).toBe('abcdefghij');
  });
});
