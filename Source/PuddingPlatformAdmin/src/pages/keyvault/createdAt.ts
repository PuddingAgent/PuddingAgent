/**
 * KeyVault 列表时间解析。
 *
 * 后端 KeyVaultEntity.CreatedAt 用 DateTime.UtcNow 写入，但实体列是 SQLite TEXT；
 * 读回后 DateTimeKind 变成 Unspecified，序列化时不带时区后缀，浏览器会把这段
 * UTC 时钟当成「本地时间」解析，导致列表时间比真实时间早 8 小时（+08:00 主机）。
 *
 * 这里的约定：不带时区后缀的值一律按 UTC 解释；带后缀的按其自身偏移解释。
 */
export const parseCreatedAt = (raw?: string): Date | null => {
  if (!raw) return null;
  const hasTz = /[zZ]$|[+-]\d{2}:?\d{2}$/.test(raw);
  const d = new Date(hasTz ? raw : `${raw}Z`);
  return Number.isNaN(d.getTime()) ? null : d;
};

export const formatCreatedAt = (raw?: string): string => {
  const d = parseCreatedAt(raw);
  return d ? d.toLocaleString('zh-CN') : raw || '—';
};
