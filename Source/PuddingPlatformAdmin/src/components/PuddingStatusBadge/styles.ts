/**
 * PuddingStatusBadge styles
 */

const baseBadge = {
  display: 'inline-flex',
  alignItems: 'center',
  gap: 5,
  fontSize: 12,
  lineHeight: '18px',
  padding: '1px 8px',
  borderRadius: 'var(--pudding-admin-radius)',
};

const dot = {
  width: 6,
  height: 6,
  borderRadius: '50%',
  display: 'inline-block',
};

export default {
  badge: baseBadge,
  dot,
  success: {
    ...baseBadge,
    background: 'rgba(79, 127, 88, 0.1)',
    color: 'var(--pudding-admin-success)',
    '& .dot': { background: 'var(--pudding-admin-success)' },
  },
  warning: {
    ...baseBadge,
    background: 'rgba(183, 121, 31, 0.1)',
    color: 'var(--pudding-admin-warning)',
    '& .dot': { background: 'var(--pudding-admin-warning)' },
  },
  danger: {
    ...baseBadge,
    background: 'rgba(180, 35, 24, 0.1)',
    color: 'var(--pudding-admin-danger)',
    '& .dot': { background: 'var(--pudding-admin-danger)' },
  },
  neutral: {
    ...baseBadge,
    background: 'var(--pudding-admin-surface-muted)',
    color: 'var(--pudding-admin-text-muted)',
    '& .dot': { background: 'var(--pudding-admin-text-muted)' },
  },
  accent: {
    ...baseBadge,
    background: 'var(--pudding-admin-accent-soft)',
    color: 'var(--pudding-admin-accent)',
    '& .dot': { background: 'var(--pudding-admin-accent)' },
  },
};
