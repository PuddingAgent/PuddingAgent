import type { KeyVaultUsageEntryDto } from '@/services/platform/api';

export type UsageBadge = {
  key: 'orphan' | 'references' | 'duplicate';
  color: 'red' | 'green' | 'purple';
  label: string;
  tooltip: string;
};

/**
 * 把服务端的 usage 条目翻译成「被引用」列的徽标。
 *
 * 抽成纯函数是为了让三个判定可以被测试守住（孤儿 / 被引用 / 同值重复）。
 * 它们直接决定用户会不会去改一条其实没有任何配置在用的密钥，判断错了不会报错，
 * 只会让人白忙一场 —— 所以需要测试而不是靠读代码。
 */
export function describeUsage(
  usage: KeyVaultUsageEntryDto | undefined | null,
): UsageBadge[] | null {
  if (!usage) return null;

  const badges: UsageBadge[] = [];

  if (usage.referenceCount === 0) {
    badges.push({
      key: 'orphan',
      color: 'red',
      label: '孤儿',
      tooltip: '没有任何配置引用它，改它不会影响任何行为。',
    });
  } else {
    const files = (usage.referencedBy ?? []).filter((f) => !!f);
    badges.push({
      key: 'references',
      color: 'green',
      label: `${usage.referenceCount} 处`,
      // 数量与清单不一致时要说清楚，不要给一个空 tooltip 让人误以为没有引用者。
      tooltip: files.length ? files.join('\n') : '（服务端未返回引用文件清单）',
    });
  }

  if (usage.duplicateGroupId) {
    badges.push({
      key: 'duplicate',
      color: 'purple',
      label: '重复',
      tooltip: '与另一条密钥的值完全相同。它们是两条独立条目，改一条不会同步另一条。',
    });
  }

  return badges;
}
