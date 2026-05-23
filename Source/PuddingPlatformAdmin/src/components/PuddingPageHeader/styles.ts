/**
 * PuddingPageHeader styles
 *
 * 使用 CSS-in-JS 对象方式以兼容当前项目的样式模式。
 * Token 引用自 --pudding-admin-* CSS 变量。
 */

export default {
  header: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'flex-start',
    flexWrap: 'wrap' as const,
    gap: 12,
    padding: '24px 28px 12px',
  },
  copy: {
    flex: 1,
    minWidth: 0,
  },
  eyebrow: {
    fontSize: 12,
    color: 'var(--pudding-admin-text-muted)',
    marginBottom: 4,
    lineHeight: '18px',
  },
  title: {
    fontSize: 22,
    fontWeight: 600,
    color: 'var(--pudding-admin-text)',
    margin: 0,
    lineHeight: '32px',
  },
  description: {
    fontSize: 13,
    color: 'var(--pudding-admin-text-muted)',
    margin: '4px 0 0',
    lineHeight: '20px',
    maxWidth: 560,
  },
  meta: {
    marginTop: 8,
  },
  actions: {
    display: 'flex',
    gap: 8,
    alignItems: 'center',
    flexShrink: 0,
  },
};
