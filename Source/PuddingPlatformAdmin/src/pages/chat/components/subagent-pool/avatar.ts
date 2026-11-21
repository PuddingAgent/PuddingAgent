// ── 子代理头像：无图片资源时的确定性占位 ─────────────────────────
// 后端只给 avatarKey（可能是模型/模板标识），不一定能解析成图片；
// 这里按 name + poolId 生成稳定的「首字符 + 色相」，同一子代理每次渲染一致。
// 不引入图形库，纯 CSS（背景色相由内联 CSS 变量提供）。

export interface DerivedAvatar {
  /** 显示字符：取名字首个字素（emoji/中文安全），名字为空时退回 poolId。 */
  initial: string;
  /** 0~359 的色相，喂给 `hsl(var(--sa-avatar-hue) ...)`。 */
  hue: number;
}

/** FNV-1a 32 位字符串哈希：确定性、无依赖、分布够用。 */
export function hashAvatarSeed(seed: string): number {
  let hash = 0x811c9dc5;
  for (let i = 0; i < seed.length; i += 1) {
    hash ^= seed.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return hash >>> 0;
}

export function deriveAvatar(
  name: string | null | undefined,
  poolId: string,
): DerivedAvatar {
  const trimmed = (name ?? '').trim();
  // Array.from 按码点切分，避免把代理对（emoji）截成半个字符。
  const firstChar = Array.from(trimmed)[0] ?? Array.from(poolId)[0] ?? '?';
  return {
    initial: firstChar.toUpperCase(),
    hue: hashAvatarSeed(`${trimmed}|${poolId}`) % 360,
  };
}
