/**
 * PuddingToolbar styles
 */

export default {
  toolbar: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap' as const,
    gap: 12,
    padding: '8px 28px 12px',
    minHeight: 44,
  },
  leading: {
    flex: 1,
    minWidth: 200,
    maxWidth: 400,
  },
  filters: {
    display: 'flex',
    gap: 8,
    alignItems: 'center',
  },
  actions: {
    display: 'flex',
    gap: 8,
    alignItems: 'center',
    marginLeft: 'auto',
  },
};
