// ── 头像占位确定性测试（任务 B）──────────────────────────────────
// 同一子代理每次渲染必须得到同一首字符与色相；不同子代理不得挤在同一色相。
import { deriveAvatar, hashAvatarSeed } from './avatar';

describe('deriveAvatar', () => {
  it('同名同 poolId 结果稳定', () => {
    expect(deriveAvatar('检索助手', 'pool-1')).toEqual(
      deriveAvatar('检索助手', 'pool-1'),
    );
  });

  it('取名字首字符，名字为空时退回 poolId 首字符', () => {
    expect(deriveAvatar('评审助手', 'pool-2').initial).toBe('评');
    expect(deriveAvatar('', 'pool-2').initial).toBe('P');
    expect(deriveAvatar(null, 'zpool').initial).toBe('Z');
  });

  it('emoji 名字按码点取首字符，不截成半个代理对', () => {
    expect(deriveAvatar('🐳鲸鲸', 'pool-3').initial).toBe('🐳');
  });

  it('色相在 0~359 内且不同种子不同', () => {
    const a = deriveAvatar('检索助手', 'pool-1').hue;
    const b = deriveAvatar('评审助手', 'pool-2').hue;
    expect(a).toBeGreaterThanOrEqual(0);
    expect(a).toBeLessThan(360);
    expect(b).toBeGreaterThanOrEqual(0);
    expect(b).toBeLessThan(360);
    expect(a).not.toBe(b);
  });

  it('hashAvatarSeed 对空串也返回无符号 32 位值', () => {
    expect(hashAvatarSeed('')).toBeGreaterThanOrEqual(0);
    expect(hashAvatarSeed('x')).toBeLessThan(2 ** 32);
  });
});
