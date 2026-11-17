import { describeUsage } from './usageView';
import type { KeyVaultUsageEntryDto } from '@/services/platform/api';

const base: KeyVaultUsageEntryDto = {
  name: 'cfg.demo',
  referenceCount: 1,
  referencedBy: ['config/demo.json'],
} as KeyVaultUsageEntryDto;

describe('describeUsage', () => {
  it('returns null when the server sent nothing for this entry', () => {
    expect(describeUsage(undefined)).toBeNull();
    expect(describeUsage(null)).toBeNull();
  });

  it('marks a zero-reference entry as an orphan', () => {
    const badges = describeUsage({ ...base, referenceCount: 0, referencedBy: [] })!;

    expect(badges).toHaveLength(1);
    expect(badges[0].key).toBe('orphan');
    expect(badges[0].color).toBe('red');
  });

  it('shows the reference count when the entry is used', () => {
    const badges = describeUsage({ ...base, referenceCount: 3 })!;

    expect(badges).toHaveLength(1);
    expect(badges[0].key).toBe('references');
    expect(badges[0].label).toContain('3');
  });

  it('lists the referencing files in the tooltip', () => {
    const badges = describeUsage({
      ...base,
      referenceCount: 2,
      referencedBy: ['config/a.json', 'channels/b.json'],
    })!;

    expect(badges[0].tooltip).toBe('config/a.json\nchannels/b.json');
  });

  it('never leaves an empty tooltip when the count and the list disagree', () => {
    const badges = describeUsage({ ...base, referenceCount: 2, referencedBy: [] })!;

    expect(badges[0].tooltip.length).toBeGreaterThan(0);
    expect(badges[0].tooltip).not.toBe('\n');
  });

  it('ignores blank entries in the referencing list', () => {
    const badges = describeUsage({
      ...base,
      referenceCount: 1,
      referencedBy: ['', 'config/a.json'],
    })!;

    expect(badges[0].tooltip).toBe('config/a.json');
  });

  it('adds a duplicate badge on top of the reference badge', () => {
    const badges = describeUsage({ ...base, duplicateGroupId: 'dup-1' })!;

    expect(badges.map((b) => b.key)).toEqual(['references', 'duplicate']);
  });

  it('can be both an orphan and a duplicate', () => {
    const badges = describeUsage({
      ...base,
      referenceCount: 0,
      referencedBy: [],
      duplicateGroupId: 'dup-2',
    })!;

    expect(badges.map((b) => b.key)).toEqual(['orphan', 'duplicate']);
  });
});
