/**
 * Workspace page styles
 *
 * 页面级布局样式，引用 --pudding-admin-* token。
 * 组件级样式由 wrapper components 自行管理。
 */

export default {
  shell: {
    minHeight: '100vh',
    background: 'var(--warm-beige)',
    color: 'var(--pudding-admin-text)',
  },
  content: {
    width: 'min(1180px, 100%)',
    margin: '0 auto',
    paddingBottom: 28,
  },
  page: {
    animation: 'pageEnterAdmin 120ms ease-out',
  },

  nameCell: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 2,
  },
  name: {
    fontWeight: 500,
    color: 'var(--pudding-admin-text)',
  },
  nameDescription: {
    fontSize: 12,
    color: 'var(--pudding-admin-text-muted)',
    lineHeight: '18px',
    maxWidth: 320,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap' as const,
  },

  createForm: {
    marginTop: 16,
  },

  emptyState: {
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'center',
    justifyContent: 'center',
    gap: 12,
    padding: '64px 28px',
    textAlign: 'center' as const,
  },
  emptyTitle: {
    fontSize: 16,
    fontWeight: 600,
    color: 'var(--pudding-admin-text)',
  },
  emptyDescription: {
    fontSize: 13,
    color: 'var(--pudding-admin-text-muted)',
    maxWidth: 360,
    lineHeight: '20px',
  },

  cardGrid: {
    padding: '0 28px 24px',
  },
};
