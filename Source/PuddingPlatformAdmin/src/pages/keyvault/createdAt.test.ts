import { parseCreatedAt } from './createdAt';

describe('parseCreatedAt', () => {
  it('把不带时区后缀的值按 UTC 解释（SQLite TEXT 丢失 DateTimeKind 的场景）', () => {
    const d = parseCreatedAt('2026-11-17T03:26:14');
    expect(d).not.toBeNull();
    // 03:26:14 UTC = 本地 11:26:14（+08:00）。若按本地解析，会差 8 小时。
    expect(d!.getTime()).toBe(Date.UTC(2026, 10, 17, 3, 26, 14));
  });

  it('带 Z 后缀的按其自身时区解释', () => {
    expect(parseCreatedAt('2026-11-17T03:26:14Z')!.getTime()).toBe(
      Date.UTC(2026, 10, 17, 3, 26, 14),
    );
  });

  it('带偏移量的按其偏移解释', () => {
    expect(parseCreatedAt('2026-11-17T11:26:14+08:00')!.getTime()).toBe(
      Date.UTC(2026, 10, 17, 3, 26, 14),
    );
  });

  it('空值与非法值返回 null', () => {
    expect(parseCreatedAt('')).toBeNull();
    expect(parseCreatedAt(undefined)).toBeNull();
    expect(parseCreatedAt('not-a-date')).toBeNull();
  });
});
