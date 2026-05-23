/**
 * PuddingEntityCard styles
 */

export default {
  card: {
    borderRadius: 'var(--pudding-admin-radius)',
    background: 'var(--pudding-admin-surface)',
    border: '1px solid var(--pudding-admin-border)',
    height: '100%',
  },
  header: {
    marginBottom: 12,
  },
  titleRow: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    gap: 8,
    marginBottom: 4,
  },
  title: {
    fontSize: 15,
    fontWeight: 600,
    color: 'var(--pudding-admin-text)',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap' as const,
  },
  status: {
    flexShrink: 0,
  },
  description: {
    fontSize: 12,
    color: 'var(--pudding-admin-text-muted)',
    lineHeight: '18px',
    margin: '4px 0 0',
  },
  metaGrid: {
    display: 'flex',
    gap: 16,
    flexWrap: 'wrap' as const,
    marginBottom: 12,
  },
  metaItem: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 2,
  },
  metaLabel: {
    fontSize: 11,
    color: 'var(--pudding-admin-text-muted)',
    lineHeight: '16px',
  },
  metaValue: {
    fontSize: 13,
    color: 'var(--pudding-admin-text)',
    fontWeight: 500,
    lineHeight: '20px',
  },
  actions: {
    display: 'flex',
    gap: 8,
    borderTop: '1px solid var(--pudding-admin-border)',
    paddingTop: 12,
  },
};
